using Privon.App;
using Privon.Core;
using Privon.Detection;
using Privon.Windows;

namespace Privon.App.Tests;

// SYNTHETIC DATA ONLY. When the user presses "모두 보호" the resolver re-captures the foreground and the Windows layer re-checks it before reading and
// writing the clipboard. This file compares what happens for each foreground situation, through the REAL coordinator, resolver, decision scope,
// prompt coordinator and tracker, with a synthetic foreground provider. It does not show a window, send input, or touch the real clipboard.
//
// PRIVON 0.4.0, policy B. A mouse click on the non-activating prompt leaves the supported app in the foreground. Anything that ACTIVATES the prompt (a
// keyboard user, a screen reader, a UIA client) makes PRIVON's own process the foreground, which the fresh-target rule alone would refuse. The prompt now
// reports HOW the press came about (see DecisionPromptInvocation), and the prompt coordinator turns that into one of two narrow permissions, or none:
//   * the application that was active right before the prompt's application became active IS the original supported target -> the original target stands in;
//   * a fresh Enter / Space addressed to the button raised the click -> a MANUAL decision, which claims no target at all (and is not proof of a person);
//   * anything else -- including a UIA Invoke while another app was in the foreground, or any unknown predecessor -> refused, exactly as before.
// The fake surface below states those facts directly; the real window's own evidence is tested in DecisionPromptWindowForegroundTests.
public sealed class DecisionForegroundMatrixTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) { try { d.Dispose(); } catch (Exception) { } }
    }

    private const string Gps = "37.5665,126.9780";                      // a Level 1 decision

    private static readonly ForegroundTargetSnapshot OwnPrompt = new(IsResolved: true, ProcessId: (uint)Environment.ProcessId, ProcessName: "Privon.App");
    private static readonly ForegroundTargetSnapshot OtherApp = new(IsResolved: true, ProcessId: 9999, ProcessName: "notepad");

    private sealed class Rig : IDisposable
    {
        private uint _sequence;

        private readonly ForegroundTargetSnapshot? _shownFor;

        /// <param name="shownFor">The foreground the decision is shown for (what the prompt coordinator records); the default is the supported app.</param>
        public Rig(ForegroundTargetSnapshot? shownFor = null, bool withCapture = true)
        {
            _shownFor = shownFor;
            Lifecycle = new ClipboardDecisionScopeLifecycle();
            Publisher = new ClipboardDecisionSessionPublisher(Lifecycle);
            Tracker = new ClipboardProtectionStatusTracker(scanWatchdog: Timeout.InfiniteTimeSpan);
            Write = new FakeClipboardWriteTransport { NextResult = ClipboardWriteResult.Success(resultSequence: 2) };
            var gate = new ClipboardOperationGate();
            var processor = new ClipboardPrivacyProcessor(DetectionPipeline.CreateDefault, new FakeTrustExceptionProvider());
            var handoff = new FakeClipboardComposerVerificationHandoff();
            Handoff = handoff;
            Coordinator = new ClipboardPrivacyCoordinator(
                Read, Target, processor, Write, Lifecycle, Publisher, gate, handoff, new FakeClipboardComposerVerificationInvalidation(),
                diagnostics: Tracker, foregroundTrigger: Foreground);
            Coordinator.Start();
            Resolver = new ClipboardDecisionActionResolver(gate, Lifecycle, Target, Read, Write, processor, handoff, diagnostics: Tracker);
            // the prompt coordinator records the foreground as the decision is shown; a test can make that foreground something else
            var capture = new SwitchableCapture(Target, () => _shownFor);
            Prompts = new DecisionPromptCoordinator(Publisher, Lifecycle, Resolver, new FakeDispatcherScheduler { RunSynchronously = true }, () => Surface = new FakeDecisionPromptSurface(), withCapture ? capture : null);
            Prompts.Start();
        }

        public FakeClipboardReadTransport Read { get; } = new();
        public FakeForegroundTargetCapture Target { get; } = new();
        public FakeClipboardForegroundTrigger Foreground { get; } = new();
        public FakeClipboardWriteTransport Write { get; }
        public ClipboardDecisionScopeLifecycle Lifecycle { get; }
        public ClipboardDecisionSessionPublisher Publisher { get; }
        public ClipboardProtectionStatusTracker Tracker { get; }
        public ClipboardPrivacyCoordinator Coordinator { get; }
        public ClipboardDecisionActionResolver Resolver { get; }
        public DecisionPromptCoordinator Prompts { get; }
        public FakeDecisionPromptSurface? Surface { get; private set; }
    public FakeClipboardComposerVerificationHandoff Handoff { get; private set; } = null!;

        public async Task CopyAsync(string text)
        {
            long before = Tracker.Current.CompletedAttempts;
            uint sequence = ++_sequence;
            Read.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(sequence, true, text));
            Read.RaiseChanged(new ClipboardChangeNotification(sequence, true, true));
            await WaitAsync(() => Tracker.Current.CompletedAttempts > before);
        }

        /// <summary>The user presses the one button, and the result of that press is awaited.</summary>
        public async Task PressProtectAllAsync()
        {
            var surface = Surface ?? throw new InvalidOperationException("no prompt is showing");
            surface.RaiseProtectAllRequested();
            await WaitAsync(() => surface.IsClosed || surface.LastNeutralFailureMessage is not null);
        }

        public ProtectionStatusPresentation Shown => ProtectionStatusPresenter.Present(Tracker.Current);

        public void Dispose()
        {
            Prompts.Dispose();
            Coordinator.Dispose();
        }
    }

    /// <summary>The capture the prompt coordinator uses when a decision is published: the shared fake target, unless the test says the decision was shown for another foreground.</summary>
    private sealed class SwitchableCapture(FakeForegroundTargetCapture inner, Func<ForegroundTargetSnapshot?> shownFor) : IForegroundTargetCapture
    {
        public ForegroundTargetSnapshot Capture() => shownFor() ?? inner.Capture();
    }

    private async Task<Rig> PendingAsync()
    {
        var rig = new Rig();
        _disposables.Add(rig);
        _disposables.Add(rig.Tracker);
        await rig.CopyAsync(Gps);
        Assert.NotNull(rig.Surface);
        Assert.Equal(1, rig.Surface!.ShowCallCount);                          // the prompt is up, the decision is pending, the raw text is on the clipboard
        Assert.Contains("Awaiting your decision", rig.Shown.Tooltip);
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

    // ===== 1. the supported app stays in the foreground: what a MOUSE click does ================================================

    [Fact]
    public async Task WhenTheSupportedAppStaysInTheForeground_ProtectAllProtectsAndTheStatusIsVerified()
    {
        var rig = await PendingAsync();

        await rig.PressProtectAllAsync();

        Assert.True(rig.Surface!.IsClosed);
        Assert.Null(rig.Surface.LastNeutralFailureMessage);
        Assert.Equal(1, rig.Write.CallCount);
        Assert.DoesNotContain(Gps, rig.Write.ReceivedReplacementTexts[0]);
        Assert.Equal(ProtectionState.Verified, rig.Tracker.Current.State);
    }

    // ===== 2. PRIVON's OWN window is the foreground: what a KEYBOARD user or a SCREEN READER produces ===========================

    [Fact]
    public async Task WhenPrivonsOwnWindowIsTheForeground_ProtectAllIsRefused_NothingIsWritten_AndThePromptCannotBePressedAgain()
    {
        var rig = await PendingAsync();
        rig.Target.SnapshotToReturn = OwnPrompt;                              // activating the prompt (which keyboard or a screen reader needs) makes PRIVON the foreground

        await rig.PressProtectAllAsync();

        // CHARACTERIZATION of the accessibility defect: this is the only way a non-mouse user can reach the button, and it is always refused.
        Assert.NotNull(rig.Surface!.LastNeutralFailureMessage);
        Assert.False(rig.Surface.IsClosed);
        Assert.Equal(0, rig.Write.CallCount);
        Assert.Contains("Awaiting your decision", rig.Shown.Tooltip);                       // the raw text is still there and the status says the decision is still open
        Assert.True(rig.Tracker.Current.UnprotectedTextMayRemain);
        Assert.Equal(1, rig.Surface.DisableProtectActionCallCount);          // and the button is now disabled: there is no way to try again on this prompt
        Assert.True(rig.Lifecycle.HasActiveScope);                            // while the decision itself is still answerable
    }

    [Fact]
    public async Task TheForegroundMovingToPrivonDuringTheAttempt_IsRefusedAtTheGuardedRead_NothingIsWritten()
    {
        // The real-machine symptom: the target is captured as the supported app, then the prompt becomes the foreground a moment later and the
        // Windows layer's mechanical check reports TargetChanged on the read.
        var rig = await PendingAsync();
        rig.Read.NextReadResult = ClipboardTextReadResult.Failure(ClipboardReadOutcome.TargetChanged);

        await rig.PressProtectAllAsync();

        Assert.NotNull(rig.Surface!.LastNeutralFailureMessage);
        Assert.Equal(0, rig.Write.CallCount);
        Assert.Contains("Awaiting your decision", rig.Shown.Tooltip);
    }

    // ===== 3. a genuinely different app is the foreground: must stay refused ====================================================

    [Fact]
    public async Task WhenAnUnsupportedAppIsTheForeground_ProtectAllIsRefused_AndNothingIsWritten()
    {
        var rig = await PendingAsync();
        rig.Target.SnapshotToReturn = OtherApp;

        await rig.PressProtectAllAsync();

        Assert.NotNull(rig.Surface!.LastNeutralFailureMessage);
        Assert.Equal(0, rig.Write.CallCount);
        Assert.Contains("Awaiting your decision", rig.Shown.Tooltip);
    }

    // ===== 4. the clipboard revision changed under the decision =================================================================

    [Fact]
    public async Task WhenTheClipboardTextChangedUnderTheDecision_ProtectAllIsRefused_AndTheNewTextIsNeverOverwritten()
    {
        var rig = await PendingAsync();
        rig.Read.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(99, true, "회의는 오후 세 시입니다"));   // no notification reached the scope

        await rig.PressProtectAllAsync();

        Assert.NotNull(rig.Surface!.LastNeutralFailureMessage);
        Assert.Equal(0, rig.Write.CallCount);                                 // the newer text was not rewritten from the older decision
    }

    // ===== 5. the decision scope expired =========================================================================================

    [Fact]
    public async Task AfterANewCopy_TheOldDecisionIsStale_AndCannotRewriteAnything()
    {
        var rig = await PendingAsync();
        var oldScope = rig.Lifecycle.GetActiveScope()!;
        var oldItem = oldScope.Items.Single();
        await rig.CopyAsync("회의는 오후 세 시입니다");                          // nothing to decide about: the old scope ends

        var result = await rig.Resolver.ResolveAsync(oldScope, oldItem, ClipboardDecisionIntent.Protect);

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, result.Outcome);
        Assert.Equal(0, rig.Write.CallCount);
        Assert.DoesNotContain("Awaiting your decision", rig.Shown.Tooltip);
    }

    // ===== policy B: what an own-prompt press may stand in for ======================================================================

    private static readonly ForegroundTargetSnapshot Supported = new FakeForegroundTargetCapture().SnapshotToReturn;

    /// <summary>Stands in for the Alt+Tab switcher (explorer): neither the supported app nor an external app.</summary>
    private const uint SwitcherPid = 777;

    private static async Task PressAsync(Rig rig, bool foreground = true, bool byKeyboard = false, uint previous = 0, ForegroundTargetSnapshot? foregroundNow = null)
    {
        rig.Surface!.IsForeground = foreground;
        rig.Surface.ByKeyboard = byKeyboard;
        rig.Surface.PreviousActiveProcessId = previous;
        rig.Target.SnapshotToReturn = foregroundNow ?? OwnPrompt;             // activating the prompt makes PRIVON's own process the foreground
        await rig.PressProtectAllAsync();
    }

    private static void AssertRefusedNothingWritten(Rig rig)
    {
        Assert.NotNull(rig.Surface!.LastNeutralFailureMessage);
        Assert.False(rig.Surface.IsClosed);
        Assert.Equal(0, rig.Write.CallCount);
        Assert.False(rig.Tracker.Current.ClaimsProtected);
        Assert.True(rig.Tracker.Current.UnprotectedTextMayRemain);
    }

    // --- 2. a UIA press: allowed ONLY when the application active just before the prompt's was the original supported target -------

    [Fact]
    public async Task AUiaPress_WhenThePreviousAppWasTheSupportedTarget_ProtectsAndIsVerified()
    {
        var rig = await PendingAsync();

        await PressAsync(rig, previous: Supported.ProcessId);

        Assert.True(rig.Surface!.IsClosed);
        Assert.Null(rig.Surface.LastNeutralFailureMessage);
        Assert.Equal(1, rig.Write.CallCount);
        Assert.DoesNotContain(Gps, rig.Write.ReceivedReplacementTexts[0]);
        Assert.Equal(ProtectionState.Verified, rig.Tracker.Current.State);    // a real, read-back-verified rewrite: the normal Verified
        Assert.Equal(1, rig.Handoff.CallCount);                               // and the composer check is told about the supported app, never PRIVON's window
        Assert.Equal(Supported, rig.Handoff.ReceivedExpectedTargets[0]);
    }

    // --- 3. the defect: another app was in the foreground and a UIA Invoke activated the prompt -------------------------------------

    [Fact]
    public async Task AUiaPress_WhenAnExternalAppWasTheForeground_IsRefused_NothingIsWritten()
    {
        var rig = await PendingAsync();

        await PressAsync(rig, previous: OtherApp.ProcessId);

        AssertRefusedNothingWritten(rig);
        Assert.Contains("Awaiting your decision", rig.Shown.Tooltip);
    }

    // --- 6. no key, and no usable predecessor ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0u)]                                                          // unknown / not recorded for this activation / unresolvable
    [InlineData(SwitcherPid)]                                                 // the switcher is not the supported app, and without a key there is no manual decision
    public async Task AUiaPress_WithNoKeyAndNoSupportedPredecessor_IsRefused(uint previous)
    {
        var rig = await PendingAsync();

        await PressAsync(rig, previous: previous);

        AssertRefusedNothingWritten(rig);
    }

    // --- 4. the keyboard path: a manual decision ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AKeyboardPress_AfterAltTab_IsAManualDecision_ProtectsAndIsVerified_ButClaimsNoTarget()
    {
        var rig = await PendingAsync();

        await PressAsync(rig, byKeyboard: true, previous: SwitcherPid);       // Alt+Tab: the previous application is the switcher

        Assert.True(rig.Surface!.IsClosed);
        Assert.Null(rig.Surface.LastNeutralFailureMessage);
        Assert.Equal(1, rig.Write.CallCount);
        Assert.DoesNotContain(Gps, rig.Write.ReceivedReplacementTexts[0]);
        Assert.Equal(ProtectionState.Verified, rig.Tracker.Current.State);    // the write was read back and verified, so the ordinary Verified applies
        Assert.Equal(0, rig.Handoff.CallCount);                               // a manual decision restores no target, so the composer check is told about none
    }

    // --- 5. the same keystrokes from an external app: the identical manual decision, with no external target claimed -----------------

    [Fact]
    public async Task AKeyboardPress_ThatCameFromAnExternalApp_IsTheSameManualDecision_AndClaimsNoTargetAtAll()
    {
        var rig = await PendingAsync();

        await PressAsync(rig, byKeyboard: true, previous: OtherApp.ProcessId);

        Assert.Equal(1, rig.Write.CallCount);                                 // the manual policy applies ...
        Assert.Equal(0, rig.Handoff.CallCount);                               // ... and neither the external app nor the original app is vouched for
    }

    [Fact]
    public async Task AKeyboardPress_WhoseSupportedPredecessorIsKnown_UsesTheStrongerBasis_AndHandsTheComposerCheckTheSupportedApp()
    {
        var rig = await PendingAsync();

        await PressAsync(rig, byKeyboard: true, previous: Supported.ProcessId);

        Assert.Equal(1, rig.Write.CallCount);
        Assert.Equal(1, rig.Handoff.CallCount);
        Assert.Equal(Supported, rig.Handoff.ReceivedExpectedTargets[0]);
    }

    // --- 8/9/11/12. the manual decision does not relax anything else -----------------------------------------------------------------

    [Fact]
    public async Task TheManualDecision_NeedsTheDecisionToHaveBeenShownForASupportedApp()
    {
        var rig = new Rig(shownFor: OtherApp);                                // the foreground when the decision was shown was an unsupported app
        _disposables.Add(rig);
        _disposables.Add(rig.Tracker);
        await rig.CopyAsync(Gps);

        await PressAsync(rig, byKeyboard: true, previous: SwitcherPid);

        AssertRefusedNothingWritten(rig);
    }

    [Fact]
    public async Task TheManualDecision_StillNeedsPrivonsOwnPromptToBeTheForegroundAtTheMoment()
    {
        var rig = await PendingAsync();

        await PressAsync(rig, byKeyboard: true, previous: SwitcherPid, foregroundNow: OtherApp);   // by the time the target is captured, another app is

        AssertRefusedNothingWritten(rig);
    }

    [Fact]
    public async Task TheManualDecision_NeedsThePromptItselfToBeTheForeground()
    {
        var rig = await PendingAsync();

        await PressAsync(rig, foreground: false, byKeyboard: true, previous: SwitcherPid);

        AssertRefusedNothingWritten(rig);
    }

    [Fact]
    public async Task TheManualDecision_DoesNotCoverAClipboardThatChangedUnderTheDecision()
    {
        var rig = await PendingAsync();
        rig.Read.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(99, true, "회의는 오후 세 시입니다"));

        await PressAsync(rig, byKeyboard: true, previous: SwitcherPid);

        AssertRefusedNothingWritten(rig);                                     // the newer text is never overwritten
    }

    [Fact]
    public async Task TheManualDecision_DoesNotCoverTheForegroundMovingAwayDuringTheAttempt()
    {
        var rig = await PendingAsync();
        rig.Read.NextReadResult = ClipboardTextReadResult.Failure(ClipboardReadOutcome.TargetChanged);   // the Windows layer's own check still runs

        await PressAsync(rig, byKeyboard: true, previous: SwitcherPid);

        AssertRefusedNothingWritten(rig);
    }

    [Theory]
    [InlineData(true)]                                                        // the manual keyboard decision
    [InlineData(false)]                                                       // the original-target basis
    public async Task NeitherBasisCoversAnExpiredDecision(bool manual)
    {
        var rig = await PendingAsync();
        var oldScope = rig.Lifecycle.GetActiveScope()!;
        var oldItem = oldScope.Items.Single();
        await rig.CopyAsync("회의는 오후 세 시입니다");
        rig.Target.SnapshotToReturn = OwnPrompt;

        var result = await rig.Resolver.ResolveAsync(oldScope, oldItem, ClipboardDecisionIntent.Protect, new DecisionPromptForegroundContext(Supported, manual ? DecisionPromptAuthorityBasis.ManualKeyboardDecision : DecisionPromptAuthorityBasis.PreviousAppWasOriginalTarget));

        Assert.Equal(ClipboardDecisionActionOutcome.Stale, result.Outcome);
        Assert.Equal(0, rig.Write.CallCount);
    }

    [Fact]
    public async Task AWriteThatIsRejectedAfterTheManualDecision_IsLimited_NeverVerified()
    {
        var rig = await PendingAsync();
        rig.Write.NextResult = ClipboardWriteResult.Failure(ClipboardWriteOutcome.ReadBackMismatch, mutated: true);

        await PressAsync(rig, byKeyboard: true, previous: SwitcherPid);

        Assert.NotNull(rig.Surface!.LastNeutralFailureMessage);
        Assert.Equal(ProtectionState.Limited, rig.Tracker.Current.State);
        Assert.True(rig.Tracker.Current.UnprotectedTextMayRemain);
        Assert.False(rig.Tracker.Current.ClaimsProtected);
    }

    [Fact]
    public async Task WithoutATargetCaptureConfigured_NoBasisExists_AsBefore()
    {
        var rig = new Rig(shownFor: null, withCapture: false);
        _disposables.Add(rig);
        _disposables.Add(rig.Tracker);
        await rig.CopyAsync(Gps);

        await PressAsync(rig, byKeyboard: true, previous: Supported.ProcessId);

        AssertRefusedNothingWritten(rig);
    }

    // --- 13. repeated / concurrent presses and context reuse ---------------------------------------------------------------------------

    [Fact]
    public async Task ARepeatedPress_ConsumesTheDecisionOnce_AndReusesNoContext()
    {
        var rig = await PendingAsync();
        var surface = rig.Surface!;
        surface.IsForeground = true;
        surface.ByKeyboard = true;
        surface.PreviousActiveProcessId = SwitcherPid;
        rig.Target.SnapshotToReturn = OwnPrompt;

        surface.RaiseProtectAllRequested();
        surface.RaiseProtectAllRequested();                                   // the same press delivered twice
        surface.RaiseProtectAllRequested();
        await WaitAsync(() => surface.IsClosed);
        await Task.Delay(100);

        Assert.Equal(1, rig.Write.CallCount);
        Assert.Equal(1, surface.DisableProtectActionCallCount);
    }

    [Theory]
    [InlineData(true)]                                                        // the manual keyboard decision
    [InlineData(false)]                                                       // the original-target basis
    public async Task ASecondResolveWithTheSameContext_IsStale_AndWritesNothing(bool manual)
    {
        var rig = await PendingAsync();
        var scope = rig.Lifecycle.GetActiveScope()!;
        var item = scope.Items.Single();
        var context = new DecisionPromptForegroundContext(Supported, manual ? DecisionPromptAuthorityBasis.ManualKeyboardDecision : DecisionPromptAuthorityBasis.PreviousAppWasOriginalTarget);
        rig.Target.SnapshotToReturn = OwnPrompt;

        var first = await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect, context);
        var second = await rig.Resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.Protect, context);

        Assert.Equal(ClipboardDecisionActionOutcome.Applied, first.Outcome);
        Assert.Equal(ClipboardDecisionActionOutcome.Stale, second.Outcome);   // the permission was a property of one press, not of the context object
        Assert.Equal(1, rig.Write.CallCount);
    }

    [Fact]
    public async Task APressWhoseAuthorityIsRefused_StillShowsTheTextAsNotProtected()
    {
        var rig = await PendingAsync();

        await PressAsync(rig, previous: OtherApp.ProcessId, foregroundNow: OtherApp);

        Assert.True(rig.Tracker.Current.UnprotectedTextMayRemain);
        Assert.False(rig.Tracker.Current.ClaimsProtected);
        Assert.Contains("Awaiting your decision", rig.Shown.Tooltip);
    }

    // ===== lifecycle and honesty ================================================================================================

    [Fact]
    public async Task DisposingWhileTheAttemptIsInFlight_DoesNotThrow_AndNeverClaimsProtection()
    {
        var rig = await PendingAsync();
        rig.Read.HoldReadsUntilReleased = true;
        var surface = rig.Surface!;
        surface.RaiseProtectAllRequested();                                   // the attempt is now waiting on the guarded read
        await Task.Delay(100);

        rig.Prompts.Dispose();
        rig.Coordinator.Dispose();
        rig.Tracker.Dispose();
        rig.Read.ReleaseNextRead(ClipboardTextReadResult.Failure(ClipboardReadOutcome.TargetChanged));
        var ex = await Record.ExceptionAsync(() => Task.Delay(300));

        Assert.Null(ex);
        Assert.False(rig.Tracker.Current.ClaimsProtected);
    }

    [Fact]
    public async Task EveryRefusedPress_LeavesTheTextExactlyAsCopied_AndTheStatusSaysItIsNotProtected()
    {
        foreach (var scenario in new[] { OwnPrompt, OtherApp })
        {
            var rig = await PendingAsync();
            rig.Target.SnapshotToReturn = scenario;

            await rig.PressProtectAllAsync();

            Assert.False(rig.Tracker.Current.ClaimsProtected);
            Assert.True(rig.Tracker.Current.UnprotectedTextMayRemain);
            Assert.DoesNotContain("rewrite to protected form verified", rig.Shown.Tooltip);
            Assert.Equal(0, rig.Write.CallCount);
        }
    }

    // ===== the structural facts the comparison rests on =========================================================================

    [Fact]
    public void TheDecisionScopeKeepsNoTarget_SoTheResolverCanOnlyJudgeTheForegroundItSeesNow()
    {
        var scopeSource = File.ReadAllText(FindSource("ClipboardDecisionScope.cs"));

        Assert.Contains("no <c>ForegroundTargetSnapshot</c>", scopeSource);
    }

    [Fact]
    public void ThePromptWindowReportsHowItWasPressed_AndStillNeverActivatesItselfOrRestoresAnyForeground()
    {
        string source = File.ReadAllText(FindSource("DecisionPromptWindow.cs"));

        Assert.Contains("ShowActivated = false", source);
        Assert.Contains("WS_EX_NOACTIVATE", source);
        Assert.Contains("WM_ACTIVATEAPP", source);                // the one activation message it reads: the previous application's THREAD id
        Assert.Contains("ResolveProcessIdOfThread", source);      // resolved to a process, never read as one
        Assert.Contains("PreviewKeyDown", source);                // a fresh Enter ...
        Assert.Contains("PreviewKeyUp", source);                  // ... or Space, addressed to the button
        Assert.Contains("handledEventsToo: true", source);        // and the routing flag is always cleared at the end of that routing
        Assert.Contains("if (e.IsRepeat) return false;", source);   // a repeat of a key pressed before the focus arrived is never a fresh press
        Assert.Contains("ModifierKeys.Alt | ModifierKeys.Control | ModifierKeys.Windows", source);   // and Alt / Ctrl / Windows chords are never one
        Assert.DoesNotContain("OnActivated", source);
        Assert.DoesNotContain("SetForegroundWindow", source);     // nothing here brings any window forward or sends the previous one back
        Assert.DoesNotContain(".Activate()", source);
        Assert.False(System.Text.RegularExpressions.Regex.IsMatch(source, @"\bWM_ACTIVATE\b"));   // WM_ACTIVATE's lParam is useless across processes (0)
    }

    [Fact]
    public void TheCoordinatorBuildsTheContextAtThePress_AndKeepsItInNoField()
    {
        string source = File.ReadAllText(FindSource("DecisionPromptCoordinator.cs"));

        Assert.DoesNotContain("private DecisionPromptForegroundContext", source);
        Assert.DoesNotContain("static DecisionPromptForegroundContext", source.Replace("static DecisionPromptForegroundContext? BuildPromptContext", ""));
        Assert.DoesNotContain("ForegroundEpoch", source);          // the asynchronous foreground history is not an authority
    }

    private static string FindSource(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Privon.App", file))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "Privon.App", file);
    }
}
