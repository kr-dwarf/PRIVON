using System.Reflection;
using Privon.App;
using Privon.Core;
using Privon.Detection;
using Privon.Storage;
using Privon.Windows;

namespace Privon.App.Tests;

// SYNTHETIC DATA ONLY. The status warning in the SHIPPED default App: a protection rewrite that failed, an exception, a pending decision and a verified
// rewrite reach the user through the existing tracker, presenter and tray -- and nothing more is claimed than that.
//
// Not covered here and only checkable on a real machine: the WinForms NotifyIcon itself (tooltip length, balloon rendering, Focus Assist suppression),
// a real clipboard, and a real foreground change. This is a status display. The product still cannot block a paste or a send, and "Verified" is
// the rewrite-and-read-back of the LAST processed clipboard text, never a statement about what was or will be pasted or sent.
public sealed class ShippedProtectionWarningTests : IDisposable
{
    private readonly List<string> _roots = [];
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) { try { d.Dispose(); } catch (Exception) { } }
        foreach (var r in _roots)
        {
            try { Directory.Delete(r, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static ClipboardDiagnosticEvent Receive(long id) => ClipboardDiagnosticEvent.AppClipboardChangeReceived(id, (uint)id, true, true);

    private static ClipboardDiagnosticEvent Terminal(long id, ClipboardDiagnosticTerminalReason reason) =>
        ClipboardDiagnosticEvent.AttemptTerminal(id, reason, reason == ClipboardDiagnosticTerminalReason.ProcessingException ? "SyntheticException" : null);

    /// <summary>The composition the shipped launch builds: the public production constructor does exactly this, with no pipeline source and no opt-in.</summary>
    private PrivonAppComposition StartedShipped()
    {
        string root = Path.Combine(Path.GetTempPath(), "ShippedProtectionWarning", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        var composition = new PrivonAppComposition(
            root, new SessionLockNotificationAdapter(), new ClipboardChangeMonitor(), new ComposerTextReader(), new ForegroundChangeMonitor());
        _disposables.Add(composition);
        composition.Start();
        return composition;
    }

    private (PrivonAppUiBridge Bridge, FakeTrayIconSurface Tray) BridgeFor(ClipboardProtectionStatusTracker? tracker)
    {
        string root = Path.Combine(Path.GetTempPath(), "ShippedProtectionWarning", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        var store = PrivonLocalStore.OpenOrCreate(root);
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var tray = new FakeTrayIconSurface();
        var bridge = new PrivonAppUiBridge(
            new ClipboardDecisionSessionPublisher(lifecycle), lifecycle, new FakeClipboardDecisionResolver(),
            new FakeDispatcherScheduler { RunSynchronously = true }, tray, () => new FakeDecisionPromptSurface(),
            new WindowsAutoStartCoordinator(new FakeWindowsAutoStartRegistration(), () => "C:\\PRIVON\\PRIVON.exe"),
            new ProtectionCategorySettingsService(store), new UserExceptionService(store), DetectionPipeline.CreateDefault(),
            () => store.IsMasterKeyUnavailable, () => new FakeSettingsSurface(),
            new NativeMessagingHostRegistrationCoordinator(new FakeNativeMessagingHostRegistrationEnvironment()),
            protectionStatus: tracker);
        _disposables.Add(bridge);
        return (bridge, tray);
    }

    private static object? Field(object owner, string name) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);

    // ===== RED: the shipped path has no status at all ======================================================================

    [Fact]
    public void TheShippedComposition_BuildsTheProtectionStatusTracker()
    {
        var shipped = StartedShipped();

        Assert.Null(shipped.DetectionPipelineSource);          // this is the shipped default: no opt-in of any kind
        Assert.NotNull(shipped.ProtectionStatus);
    }

    [Fact]
    public void TheShippedCoordinatorAndResolver_ReportToThatTracker()
    {
        var shipped = StartedShipped();

        var tracker = shipped.ProtectionStatus;
        Assert.NotNull(tracker);
        Assert.Same(tracker, Field(Field(shipped, "_coordinator")!, "_diagnostics"));
        Assert.Same(tracker, Field(shipped.Resolver!, "_diagnostics"));
    }

    [Fact]
    public void AFailureInTheShippedPath_ReachesTheTrayAsAWarning()
    {
        var shipped = StartedShipped();
        var tracker = shipped.ProtectionStatus;
        Assert.NotNull(tracker);
        var (bridge, tray) = BridgeFor(tracker);
        bridge.Start();

        tracker!.Record(Receive(1));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.WriteRejected));

        var last = tray.ProtectionStatusHistory[^1];
        Assert.Equal(ProtectionState.Limited, last.Presentation.State);
        Assert.True(last.Presentation.IsWarning);
        Assert.Contains("PRIVON does not currently block sending", last.Presentation.Detail);
        Assert.Contains(tray.ProtectionStatusHistory, h => h.Notify);
    }

    // ===== the tray, tooltip and balloon behavior (a manual timer replaces the real one) ======================================

    private sealed class ManualTimers
    {
        public List<ManualTimer> Started { get; } = [];

        public IDisposable Start(TimeSpan delay, Action action)
        {
            var timer = new ManualTimer(delay, action);
            Started.Add(timer);
            return timer;
        }

        public void FireAll()
        {
            foreach (var timer in Started.ToList()) timer.Fire();
        }
    }

    private sealed class ManualTimer(TimeSpan delay, Action action) : IDisposable
    {
        public TimeSpan Delay { get; } = delay;
        public bool Disposed { get; private set; }
        public bool Fired { get; private set; }

        public void Fire()
        {
            if (Disposed) return;
            Fired = true;
            action();
        }

        /// <summary>Fires even if it was disposed: models a timer callback that was already queued when the timer was withdrawn.</summary>
        public void FireEvenIfDisposed()
        {
            Fired = true;
            action();
        }

        public void Dispose() => Disposed = true;
    }

    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(2);

    private (PrivonAppUiBridge Bridge, FakeTrayIconSurface Tray, ManualTimers Timers, ClipboardProtectionStatusTracker Tracker) NewDisplay(bool withDelay = true)
    {
        var tracker = new ClipboardProtectionStatusTracker(scanWatchdog: Timeout.InfiniteTimeSpan);
        _disposables.Add(tracker);
        var timers = new ManualTimers();
        string root = Path.Combine(Path.GetTempPath(), "ShippedProtectionWarning", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        var store = PrivonLocalStore.OpenOrCreate(root);
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var tray = new FakeTrayIconSurface();
        var bridge = new PrivonAppUiBridge(
            new ClipboardDecisionSessionPublisher(lifecycle), lifecycle, new FakeClipboardDecisionResolver(),
            new FakeDispatcherScheduler { RunSynchronously = true }, tray, () => new FakeDecisionPromptSurface(),
            new WindowsAutoStartCoordinator(new FakeWindowsAutoStartRegistration(), () => "C:\\PRIVON\\PRIVON.exe"),
            new ProtectionCategorySettingsService(store), new UserExceptionService(store), DetectionPipeline.CreateDefault(),
            () => store.IsMasterKeyUnavailable, () => new FakeSettingsSurface(),
            new NativeMessagingHostRegistrationCoordinator(new FakeNativeMessagingHostRegistrationEnvironment()),
            protectionStatus: tracker, warningNotifyDelay: withDelay ? Delay : TimeSpan.Zero, startTimer: timers.Start);
        _disposables.Add(bridge);
        bridge.Start();
        return (bridge, tray, timers, tracker);
    }

    private static int Balloons(FakeTrayIconSurface tray) { lock (tray.ProtectionStatusHistory) return tray.ProtectionStatusHistory.Count(h => h.Notify); }

    private static ProtectionStatusPresentation Last(FakeTrayIconSurface tray) { lock (tray.ProtectionStatusHistory) return tray.ProtectionStatusHistory[^1].Presentation; }

    [Fact]
    public void ARejectedRewrite_IsShownAsLimitedAtOnce_AndRaisesOneBalloonOnlyIfItPersists()
    {
        var (_, tray, timers, tracker) = NewDisplay();

        tracker.Record(Receive(1));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.WriteRejected));

        Assert.Equal(ProtectionState.Limited, Last(tray).State);       // the tooltip is not delayed
        Assert.True(Last(tray).IsWarning);
        Assert.Equal(0, Balloons(tray));                                // the balloon waits
        timers.FireAll();
        Assert.Equal(1, Balloons(tray));
        Assert.Contains("original text may remain on the clipboard", Last(tray).Detail);
        Assert.Contains("PRIVON does not currently block sending", Last(tray).Detail);
    }

    [Fact]
    public void AProcessingException_IsShownAsError_WithoutTheExceptionNameOrAnyText()
    {
        var (_, tray, timers, tracker) = NewDisplay();

        tracker.Record(Receive(1));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.ProcessingException));
        timers.FireAll();

        Assert.Equal(ProtectionState.Error, Last(tray).State);
        Assert.Equal(1, Balloons(tray));
        Assert.DoesNotContain("SyntheticException", Last(tray).Detail + Last(tray).Tooltip);
        Assert.True(Last(tray).Tooltip.Length <= ProtectionStatusPresenter.MaxTooltipLength);
    }

    [Fact]
    public void APendingDecision_IsShownAsNeedsDecision_NeverAsProtected_AndRaisesNoBalloon()
    {
        var (_, tray, timers, tracker) = NewDisplay();

        tracker.Record(Receive(1));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.DecisionPending));

        Assert.Equal(ProtectionState.NeedsDecision, Last(tray).State);
        Assert.False(Last(tray).IsWarning);
        Assert.Contains("not yet protected", Last(tray).Tooltip);
        Assert.Contains("original text remains on the clipboard", Last(tray).Detail);
        Assert.Empty(timers.Started);                                   // the decision prompt already asks the user; no balloon is queued
        Assert.Equal(0, Balloons(tray));
    }

    [Fact]
    public void AVerifiedRewrite_IsShownAsVerified_ScopedToTheLastAttempt_NeverAsSendSafety_AndRaisesNoBalloon()
    {
        var (_, tray, timers, tracker) = NewDisplay();

        tracker.Record(Receive(1));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.Success));

        Assert.Equal(ProtectionState.Verified, Last(tray).State);
        Assert.False(Last(tray).IsWarning);
        Assert.Contains("Last rewrite", Last(tray).Tooltip);
        Assert.Contains("does not guarantee that sending is safe", Last(tray).Detail);
        Assert.Contains("PRIVON does not currently block sending", Last(tray).Detail);
        Assert.Empty(timers.Started);
        Assert.Equal(0, Balloons(tray));
    }

    [Fact]
    public void ALateResultOfAnOlderAttempt_ChangesNothingTheUserSees()
    {
        var (_, tray, timers, tracker) = NewDisplay();
        tracker.Record(Receive(1));
        tracker.Record(Receive(2));                                     // newer content arrived
        int shown = tray.ProtectionStatusHistory.Count;

        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.WriteRejected));   // attempt 1 finishes late, with a failure
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.Success));         // and again, late, with a success

        Assert.Equal(shown, tray.ProtectionStatusHistory.Count);
        Assert.Equal(ProtectionState.Scanning, Last(tray).State);
        Assert.Empty(timers.Started);
    }

    [Fact]
    public void MovingToAnUnsupportedApp_NeverReusesAnEarlierVerified_AndNeverShowsTheRewriteAsConfirmed()
    {
        var (_, tray, timers, tracker) = NewDisplay();
        tracker.Record(Receive(1));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.Success));
        Assert.Equal(ProtectionState.Verified, Last(tray).State);

        tracker.Record(Receive(2));                                      // something is copied
        Assert.Equal(ProtectionState.Scanning, Last(tray).State);        // the earlier Verified is gone at once
        tracker.Record(Terminal(2, ClipboardDiagnosticTerminalReason.UnauthorizedTarget));   // but the foreground app is not supported: nothing is scanned

        Assert.NotEqual(ProtectionState.Verified, Last(tray).State);
        Assert.False(tracker.Current.ClaimsProtected);
        Assert.DoesNotContain("rewrite to protected form verified", Last(tray).Tooltip);
        Assert.Equal(0, Balloons(tray));
        Assert.Empty(timers.Started);
    }

    [Fact]
    public void AnUnsupportedAppAfterAFailure_DoesNotReleaseTheWarning()
    {
        var (_, tray, _, tracker) = NewDisplay();
        tracker.Record(Receive(1));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.WriteRejected));

        tracker.Record(Receive(2));
        tracker.Record(Terminal(2, ClipboardDiagnosticTerminalReason.UnauthorizedTarget));

        Assert.True(tracker.Current.UnresolvedFailure);
        Assert.True(Last(tray).IsWarning);
    }

    // ---- the balloon is limited to real, lasting, new warnings -----------------------------------------------------------

    [Fact]
    public void AFailureThatHealsWithinTheDelay_RaisesNoBalloon_AndTheTimerIsWithdrawn()
    {
        var (_, tray, timers, tracker) = NewDisplay();

        tracker.Record(Receive(1));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.ReadRejected));   // the clipboard was busy ...
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.Success));        // ... and the retry succeeded

        Assert.All(timers.Started, t => Assert.True(t.Disposed));       // withdrawn at once, BEFORE anything fires: not merely ignored later
        timers.FireAll();
        Assert.Equal(0, Balloons(tray));
        Assert.Equal(ProtectionState.Verified, Last(tray).State);
    }

    [Fact]
    public void ATimerCallbackAlreadyQueued_ChecksTheCurrentStatus_NotTheOneThatStartedIt()
    {
        var (_, tray, timers, tracker) = NewDisplay();
        tracker.Record(Receive(1));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.WriteRejected));
        var timer = Assert.Single(timers.Started);

        tracker.Record(Receive(2));
        tracker.Record(Terminal(2, ClipboardDiagnosticTerminalReason.NoActionRequired));   // the failure healed
        timer.FireEvenIfDisposed();                                                       // but the callback was already on its way

        Assert.Equal(0, Balloons(tray));
    }

    [Fact]
    public void ARepeatedIdenticalFailure_RaisesNoSecondBalloon()
    {
        var (_, tray, timers, tracker) = NewDisplay();
        tracker.Record(Receive(1));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.WriteRejected));
        timers.FireAll();

        tracker.Record(Receive(2));                                      // a scan in between
        tracker.Record(Terminal(2, ClipboardDiagnosticTerminalReason.WriteRejected));
        tracker.Record(Receive(3));
        tracker.Record(Terminal(3, ClipboardDiagnosticTerminalReason.WriteRejected));
        timers.FireAll();

        Assert.Equal(1, Balloons(tray));
    }

    [Fact]
    public void ALimitedFailureThatBecomesAnError_BeforeItsBalloon_RaisesOneBalloon_AboutTheError()
    {
        var (_, tray, timers, tracker) = NewDisplay();
        tracker.Record(Receive(1));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.WriteRejected));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.ProcessingException));   // the SAME attempt, retried, ends differently

        Assert.Single(timers.Started);                                   // one pending balloon per episode, not one per change of state
        timers.FireAll();
        Assert.Equal(1, Balloons(tray));
        lock (tray.ProtectionStatusHistory) Assert.Equal(ProtectionState.Error, tray.ProtectionStatusHistory.Single(h => h.Notify).Presentation.State);
    }

    [Fact]
    public void AnErrorAfterANotifiedLimited_UpdatesTheTooltip_ButAScanAlwaysSitsBetween_SoNoSecondBalloon()
    {
        // Pinned as it is: the coordinator reports a "scan started" event before every terminal one, and the existing presenter does not re-alert
        // across a scan. The user already holds a warning that says the protection is unconfirmed and the text may remain; the tooltip shows Error.
        var (_, tray, timers, tracker) = NewDisplay();
        tracker.Record(Receive(1));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.WriteRejected));
        timers.FireAll();
        tracker.Record(Receive(2));
        tracker.Record(Terminal(2, ClipboardDiagnosticTerminalReason.ProcessingException));
        timers.FireAll();

        Assert.Equal(ProtectionState.Error, Last(tray).State);
        Assert.Equal(1, Balloons(tray));
    }

    [Fact]
    public void ANewFailureAfterARealRecovery_IsANewEpisode_WithItsOwnBalloon()
    {
        var (_, tray, timers, tracker) = NewDisplay();
        tracker.Record(Receive(1));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.WriteRejected));
        timers.FireAll();
        tracker.Record(Receive(2));
        tracker.Record(Terminal(2, ClipboardDiagnosticTerminalReason.Success));       // a real recovery
        tracker.Record(Receive(3));
        tracker.Record(Terminal(3, ClipboardDiagnosticTerminalReason.WriteRejected));
        timers.FireAll();

        Assert.Equal(2, Balloons(tray));
    }

    [Fact]
    public void DisposingWhileABalloonIsPending_WithdrawsIt_AndALaterFireDoesNothing()
    {
        var (bridge, tray, timers, tracker) = NewDisplay();
        tracker.Record(Receive(1));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.WriteRejected));
        var timer = Assert.Single(timers.Started);

        bridge.Dispose();
        bridge.Dispose();                                                // idempotent
        var ex = Record.Exception(() => timer.FireEvenIfDisposed());

        Assert.Null(ex);
        Assert.True(timer.Disposed);
        Assert.Equal(0, Balloons(tray));
    }

    [Fact]
    public void ATrayThatCannotShowTheBalloon_NeverReachesProtection()
    {
        var (_, tray, timers, tracker) = NewDisplay();
        tracker.Record(Receive(1));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.WriteRejected));
        tray.ThrowOnSetProtectionStatus = new InvalidOperationException("synthetic tray failure");

        var ex = Record.Exception(() => timers.FireAll());

        Assert.Null(ex);
        Assert.Equal(ProtectionState.Limited, tracker.Current.State);     // protection state is untouched by a display failure
    }

    [Fact]
    public void WithoutADelay_TheBalloonIsImmediate_AsItWasForTheOptInHost()
    {
        var (_, tray, timers, tracker) = NewDisplay(withDelay: false);

        tracker.Record(Receive(1));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.WriteRejected));

        Assert.Equal(1, Balloons(tray));
        Assert.Empty(timers.Started);
    }

    [Fact]
    public void TheShippedBridge_UsesAShortNonZeroDelay_NotAnImmediateBalloon()
    {
        Assert.True(PrivonAppUiBridge.ProductionWarningNotifyDelay >= TimeSpan.FromSeconds(1));
        Assert.True(PrivonAppUiBridge.ProductionWarningNotifyDelay <= TimeSpan.FromSeconds(5));   // a persistent failure must not wait long
        Assert.Contains("ProductionWarningNotifyDelay,", AppSource("PrivonAppUiBridge.cs"));
    }

    private static string AppSource(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Privon.App", file))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "Privon.App", file));
    }

    // ===== end to end: the real coordinator and the shipped DEFAULT (Korean / Global) pipeline, the real tracker, the real bridge ==========

    private sealed class Flow : IDisposable
    {
        private uint _sequence;

        public Flow(ShippedProtectionWarningTests owner, ClipboardWriteResult? writeResult = null, Exception? writeThrows = null)
        {
            Write = new FakeClipboardWriteTransport { NextResult = writeResult ?? ClipboardWriteResult.Success(resultSequence: 2), ThrowOnWrite = writeThrows };
            var display = owner.NewDisplay();
            Bridge = display.Bridge;
            Tray = display.Tray;
            Timers = display.Timers;
            Tracker = display.Tracker;
            Lifecycle = new ClipboardDecisionScopeLifecycle();
            Coordinator = new ClipboardPrivacyCoordinator(
                Read, Target, new ClipboardPrivacyProcessor(DetectionPipeline.CreateDefault, new FakeTrustExceptionProvider()), Write, Lifecycle,
                new ClipboardDecisionSessionPublisher(Lifecycle), new ClipboardOperationGate(), new FakeClipboardComposerVerificationHandoff(),
                new FakeClipboardComposerVerificationInvalidation(), diagnostics: Tracker, foregroundTrigger: Foreground);
            Coordinator.Start();
        }

        public FakeClipboardReadTransport Read { get; } = new();
        public FakeForegroundTargetCapture Target { get; } = new();
        public FakeClipboardForegroundTrigger Foreground { get; } = new();
        public FakeClipboardWriteTransport Write { get; }
        public PrivonAppUiBridge Bridge { get; }
        public FakeTrayIconSurface Tray { get; }
        public ManualTimers Timers { get; }
        public ClipboardProtectionStatusTracker Tracker { get; }
        public ClipboardDecisionScopeLifecycle Lifecycle { get; }
        public ClipboardPrivacyCoordinator Coordinator { get; }

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

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met within the timeout.");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task AKoreanPhone_IsRewritten_AndTheTrayShowsAVerifiedRewrite_WithoutAnyBalloon()
    {
        using var flow = new Flow(this);

        await flow.CopyAsync("연락처 010-1234-5678");

        Assert.Equal(1, flow.Write.CallCount);
        Assert.DoesNotContain("010-1234-5678", flow.Write.ReceivedReplacementTexts[0]);
        Assert.Equal(ProtectionState.Verified, Last(flow.Tray).State);
        Assert.Equal(0, Balloons(flow.Tray));
        Assert.Empty(flow.Timers.Started);
    }

    [Fact]
    public async Task ARewriteThatIsRejected_ShowsLimited_AndTheRawTextIsReportedAsPossiblyRemaining()
    {
        using var flow = new Flow(this, ClipboardWriteResult.Failure(ClipboardWriteOutcome.InvalidText, mutated: false));

        await flow.CopyAsync("연락처 010-1234-5678");
        flow.Timers.FireAll();

        Assert.Equal(ProtectionState.Limited, Last(flow.Tray).State);
        Assert.Equal(1, Balloons(flow.Tray));
        Assert.Contains("original text may remain on the clipboard", Last(flow.Tray).Detail);
        Assert.True(flow.Tracker.Current.UnprotectedTextMayRemain);
    }

    [Fact]
    public async Task AWriteThatThrows_ShowsError_AndNeverVerified()
    {
        using var flow = new Flow(this, writeThrows: new InvalidOperationException("synthetic write failure"));

        await flow.CopyAsync("연락처 010-1234-5678");
        flow.Timers.FireAll();

        Assert.Equal(ProtectionState.Error, Last(flow.Tray).State);
        Assert.Equal(1, Balloons(flow.Tray));
        Assert.DoesNotContain("synthetic write failure", Last(flow.Tray).Detail + Last(flow.Tray).Tooltip);
        Assert.False(flow.Tracker.Current.ClaimsProtected);
    }

    [Fact]
    public async Task AResidentRegistrationNumber_WaitsForTheUser_ShownAsNeedsDecision_WithNothingWritten()
    {
        using var flow = new Flow(this);

        await flow.CopyAsync("901231-1234567");

        Assert.Equal(0, flow.Write.CallCount);
        Assert.Equal(ProtectionState.NeedsDecision, Last(flow.Tray).State);
        Assert.Equal(0, Balloons(flow.Tray));
        Assert.True(flow.Tracker.Current.UnprotectedTextMayRemain);
    }

    [Fact]
    public async Task AUsIdentifier_IsStillNotDetectedByTheShippedPipeline_SoTheTrayShowsNothingProtectedAndNothingWritten()
    {
        using var flow = new Flow(this);

        await flow.CopyAsync("SSN: 321-54-9876");

        Assert.Equal(0, flow.Write.CallCount);                                  // no U.S. detector is active in the shipped default
        Assert.NotEqual(ProtectionState.Verified, Last(flow.Tray).State);
        Assert.False(flow.Tracker.Current.ClaimsProtected);
    }

    [Fact]
    public async Task AFailureThenARealRecovery_ClearsTheWarning_AndAnEarlierVerifiedIsNeverShownForNewContent()
    {
        using var flow = new Flow(this, ClipboardWriteResult.Failure(ClipboardWriteOutcome.InvalidText, mutated: false));
        await flow.CopyAsync("연락처 010-1234-5678");
        Assert.Equal(ProtectionState.Limited, Last(flow.Tray).State);

        flow.Write.NextResult = ClipboardWriteResult.Success(resultSequence: 3);
        await flow.CopyAsync("연락처 010-7777-6666");
        Assert.Equal(ProtectionState.Verified, Last(flow.Tray).State);

        flow.Write.NextResult = ClipboardWriteResult.Failure(ClipboardWriteOutcome.InvalidText, mutated: false);
        await flow.CopyAsync("연락처 010-5555-4444");                           // new content: the earlier Verified must not survive it
        Assert.Equal(ProtectionState.Limited, Last(flow.Tray).State);
        lock (flow.Tray.ProtectionStatusHistory)
        {
            var afterThird = flow.Tray.ProtectionStatusHistory.Skip(flow.Tray.ProtectionStatusHistory.FindLastIndex(h => h.Presentation.State == ProtectionState.Verified) + 1);
            Assert.DoesNotContain(afterThird, h => h.Presentation.State == ProtectionState.Verified);
        }
    }

    [Fact]
    public async Task DisposingTheCoordinatorAndBridge_InAnyOrder_IsSafe()
    {
        var flow = new Flow(this);
        await flow.CopyAsync("연락처 010-1234-5678");

        flow.Bridge.Dispose();
        flow.Coordinator.Dispose();
        flow.Tracker.Dispose();
        var ex = Record.Exception(() => { flow.Bridge.Dispose(); flow.Coordinator.Dispose(); flow.Tracker.Dispose(); });

        Assert.Null(ex);
    }

    // ===== the shipped scope did not move =====================================================================================

    private static readonly ForegroundTargetSnapshot ChatGpt =
        new(IsResolved: true, ProcessId: 4242, ProcessName: "ChatGPT", PackageIdentity: PackageIdentityResolution.Resolved,
            PackageFamilyName: "OpenAI.Codex_2p2nqsd0c76g0");

    [Fact]
    public void TheShippedComposition_StillDetectsOnlyTheKoreanAndGlobalSet_AndOffersNoUsChoiceOrReadiness()
    {
        var shipped = StartedShipped();
        var processor = (ClipboardPrivacyProcessor)Field(shipped.Resolver!, "_processor")!;

        Assert.Equal(0, processor.Process(ChatGpt, new ClipboardTextSnapshot(1, true, "SSN: 321-54-9876")).Result.CandidateCount);
        Assert.Equal(0, processor.Process(ChatGpt, new ClipboardTextSnapshot(1, true, "MBI: 1A23C45DE67")).Result.CandidateCount);
        Assert.True(processor.Process(ChatGpt, new ClipboardTextSnapshot(1, true, "연락처 010-1234-5678")).Result.CandidateCount > 0);
        Assert.Null(shipped.DetectionPipelineSource);
        Assert.Null(shipped.UsReadinessSource);
        Assert.Null(shipped.CountryChoices);
    }

    [Fact]
    public void TheShippedAppSource_StillHasNoSendControl_AndTheTrayStillClaimsNoBlocking()
    {
        string tray = AppSource("WinFormsTrayIconSurface.cs");
        string presenter = AppSource("ProtectionStatusPresenter.cs");

        Assert.Contains("PRIVON does not currently block sending", presenter);
        Assert.DoesNotContain("has been blocked", presenter);
        Assert.DoesNotContain("RegisterHotKey", tray);
        Assert.DoesNotContain("SetWindowsHookEx", tray);
        Assert.DoesNotContain("SendInput", tray);
    }

    // ===== truthfulness: what was NOT scanned is never shown as scanned ========================================================

    private static readonly ForegroundTargetSnapshot Notepad = new(IsResolved: true, ProcessId: 9999, ProcessName: "notepad");

    [Fact]
    public async Task ACopyInAnUnsupportedApp_IsShownAsNotScanned_NeverAsAFinishedScan()
    {
        using var flow = new Flow(this);
        await flow.CopyAsync("연락처 010-1234-5678");
        Assert.Equal(ProtectionState.Verified, Last(flow.Tray).State);

        flow.Target.SnapshotToReturn = Notepad;                          // the user moves to an app PRIVON does not support ...
        await flow.CopyAsync("연락처 010-7777-6666");                       // ... and copies something there: no detection runs

        Assert.Equal(1, flow.Write.CallCount);                           // nothing was scanned or rewritten for the second copy
        var shown = Last(flow.Tray);
        Assert.DoesNotContain("Scan finished", shown.Tooltip + shown.Detail);
        Assert.DoesNotContain("rewrite to protected form verified", shown.Tooltip);             // and the earlier Verified is not reused
        Assert.Contains("was not scanned", shown.Detail);
        Assert.Contains("not scanned", shown.Tooltip);
        Assert.False(shown.IsWarning);
        Assert.True(flow.Tracker.Current.UnprotectedTextMayRemain);       // the text was left exactly as it was copied
        Assert.False(flow.Tracker.Current.ClaimsProtected);
        Assert.Equal(0, Balloons(flow.Tray));                             // a normal, frequent event: no alert
    }

    [Fact]
    public async Task NotScanningIsNotTheSameFactAsNotBlocking_AndEachIsStatedOnce()
    {
        using var flow = new Flow(this);
        flow.Target.SnapshotToReturn = Notepad;

        await flow.CopyAsync("연락처 010-1234-5678");

        var shown = Last(flow.Tray);
        Assert.DoesNotContain("block", shown.Tooltip);                      // the short text says only what was not done
        Assert.Contains("was not scanned", shown.Detail);
        Assert.Contains("PRIVON does not currently block sending", shown.Detail);   // the long text says, separately, that nothing is blocked
        Assert.True(shown.Tooltip.Length <= ProtectionStatusPresenter.MaxTooltipLength);
    }

    [Fact]
    public async Task SupportedThenUnsupportedThenSupported_EachStepShowsOnlyWhatReallyHappened()
    {
        using var flow = new Flow(this);

        await flow.CopyAsync("연락처 010-1234-5678");
        Assert.Equal(ProtectionState.Verified, Last(flow.Tray).State);

        flow.Target.SnapshotToReturn = Notepad;
        await flow.CopyAsync("연락처 010-2222-3333");
        Assert.Contains("not scanned", Last(flow.Tray).Tooltip);

        flow.Target.SnapshotToReturn = new FakeForegroundTargetCapture().SnapshotToReturn;   // back in the supported app
        await flow.CopyAsync("회의는 오후 세 시입니다");
        Assert.Contains("Scan finished", Last(flow.Tray).Tooltip);               // here a scan really ran and found nothing to protect
        Assert.DoesNotContain("not scanned", Last(flow.Tray).Tooltip);

        await flow.CopyAsync("연락처 010-4444-5555");
        Assert.Equal(ProtectionState.Verified, Last(flow.Tray).State);
        Assert.Equal(2, flow.Write.CallCount);
    }

    [Fact]
    public async Task AnUnsupportedAppCopy_NeverReleasesAnEarlierFailure_NorHidesItBehindNotScanned()
    {
        using var flow = new Flow(this, ClipboardWriteResult.Failure(ClipboardWriteOutcome.InvalidText, mutated: false));
        await flow.CopyAsync("연락처 010-1234-5678");
        flow.Timers.FireAll();
        Assert.Equal(ProtectionState.Limited, Last(flow.Tray).State);

        flow.Target.SnapshotToReturn = Notepad;
        await flow.CopyAsync("연락처 010-7777-6666");

        var shown = Last(flow.Tray);
        Assert.Equal(ProtectionState.Limited, shown.State);              // the failure is still the headline
        Assert.True(shown.IsWarning);
        Assert.DoesNotContain("not scanned", shown.Tooltip);
        Assert.True(flow.Tracker.Current.UnresolvedFailure);
        Assert.Equal(1, Balloons(flow.Tray));                             // and no second balloon for it
    }

    [Fact]
    public void ANonTextCopy_IsShownAsNotScanned_WithItsOwnReason()
    {
        var (_, tray, timers, tracker) = NewDisplay();
        tracker.Record(Receive(1));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.NonTextNotification));

        var shown = Last(tray);
        Assert.DoesNotContain("Scan finished", shown.Tooltip + shown.Detail);
        Assert.Contains("was not text", shown.Detail);
        Assert.Contains("not scanned", shown.Tooltip);
        Assert.Empty(timers.Started);
        Assert.True(tracker.Current.UnprotectedTextMayRemain);
    }

    [Fact]
    public void ALateEndOfAnOlderAttempt_DoesNotOverwriteTheNotScannedStatus()
    {
        var (_, tray, _, tracker) = NewDisplay();
        tracker.Record(Receive(1));
        tracker.Record(Receive(2));
        tracker.Record(Terminal(2, ClipboardDiagnosticTerminalReason.UnauthorizedTarget));
        int shown = tray.ProtectionStatusHistory.Count;

        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.Success));           // attempt 1 finishes late, claiming a verified rewrite
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.NoActionRequired));

        Assert.Equal(shown, tray.ProtectionStatusHistory.Count);
        Assert.Contains("not scanned", Last(tray).Tooltip);
    }

    [Fact]
    public void ANewCopy_ThatIsScanned_ClearsTheNotScannedStatus()
    {
        var (_, tray, _, tracker) = NewDisplay();
        tracker.Record(Receive(1));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.UnauthorizedTarget));
        Assert.Contains("not scanned", Last(tray).Tooltip);

        tracker.Record(Receive(2));
        Assert.DoesNotContain("not scanned", Last(tray).Tooltip);          // new content: "checking", not the old fact
        tracker.Record(Terminal(2, ClipboardDiagnosticTerminalReason.NoActionRequired));
        Assert.Contains("Scan finished", Last(tray).Tooltip);
        Assert.Equal(ProtectionState.Ready, tracker.Current.State);
        Assert.False(tracker.Current.UnprotectedTextMayRemain);
    }

    [Fact]
    public void AScannedEnd_ThatFollowsANotScannedEnd_ReplacesIt_EvenWithoutANewScanStartedEvent()
    {
        // The terminal events alone, with no "scan started" in between: the not-scanned fact must not outlive a scan that really finished.
        var (_, tray, _, tracker) = NewDisplay();
        tracker.Record(Receive(1));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.UnauthorizedTarget));
        Assert.Contains("not scanned", Last(tray).Tooltip);

        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.NoActionRequired));

        Assert.Contains("Scan finished", Last(tray).Tooltip);
        Assert.DoesNotContain("not scanned", Last(tray).Tooltip);
        Assert.False(tracker.Current.UnprotectedTextMayRemain);
    }

    [Fact]
    public void BeforeAnythingHasBeenCopied_TheTrayDoesNotSayAScanFinished()
    {
        var (_, tray, timers, tracker) = NewDisplay();

        var shown = Last(tray);
        Assert.Equal(ProtectionState.Ready, tracker.Current.State);
        Assert.Equal(0, tracker.Current.CompletedAttempts);
        Assert.DoesNotContain("Scan finished", shown.Tooltip + shown.Detail);
        Assert.Contains("Running", shown.Tooltip);
        Assert.False(shown.IsWarning);
        Assert.Empty(timers.Started);
    }

    [Fact]
    public void EveryNotScannedPresentation_FitsTheTooltip_ClaimsNothingAndIsNotAWarning()
    {
        foreach (var reason in new[] { UnscannedReason.UnsupportedTarget, UnscannedReason.NonText })
        {
            var p = ProtectionStatusPresenter.Present(new ClipboardProtectionStatus(ProtectionState.Ready, 3, false, 3, null, reason));

            Assert.True(p.Tooltip.Length <= ProtectionStatusPresenter.MaxTooltipLength);
            Assert.False(p.IsWarning);
            Assert.DoesNotContain("is protected", p.Tooltip);
            Assert.DoesNotContain("verified", p.Tooltip);
            Assert.DoesNotContain("content is safe", p.Detail);
        }
    }

    [Fact]
    public void TheShippedComposition_SharesItsForegroundTargetCapture_WithTheDecisionPrompt()
    {
        var shipped = StartedShipped();

        Assert.NotNull(shipped.TargetCapture);                                       // the ONE capture the coordinator and the resolver already use
        string bridge = AppSource("PrivonAppUiBridge.cs");
        Assert.Contains("foregroundTargetCapture: composition.TargetCapture", bridge);
        Assert.Contains("promptSurfaceFactory, foregroundTargetCapture)", bridge);
    }
}
